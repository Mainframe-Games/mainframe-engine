using System.Diagnostics;

namespace MainframeEngine.Tests.Profiling;

/// <summary>The profiler is process-wide: its tests run alone (no other collection runs in parallel with them).</summary>
[CollectionDefinition(nameof(SerialProfiler), DisableParallelization = true)]
public sealed class SerialProfiler;

[Collection(nameof(SerialProfiler))]
public sealed class ProfilerTests : IDisposable
{
    private static readonly ProfilerMarker Outer = new("Test.Outer", ProfilerCategory.Game);
    private static readonly ProfilerMarker Inner = new("Test.Inner", ProfilerCategory.Script);
    private static readonly ProfilerMarker Leaf = new("Test.Leaf");
    private static readonly ProfilerCounter Enemies = new("Test.Enemies");
    private static readonly ProfilerCounter Hits = new("Test.Hits");

    public ProfilerTests() => Profiler.ResetForTests();

    public void Dispose() => Profiler.ResetForTests();

    private static ProfilerFrame Newest()
    {
        Assert.True(Profiler.Snapshot.TryGetNewest(out var frame));
        return frame;
    }

    private static ProfilerEvent[] EventsOf(in ProfilerFrame frame)
    {
        var spans = Profiler.Snapshot.GetEvents(frame);
        var events = new ProfilerEvent[spans.Length];
        spans.CopyTo(events);
        return events;
    }

    [Fact]
    public void OffRecordsNothingAndScopesAreNoOps()
    {
        Assert.False(Profiler.IsRecording);
        using (Outer.Auto())
            Inner.Begin();
        Inner.End();
        Profiler.BeginFrame();
        Profiler.EndFrame(1f, 0f);
        Assert.Equal(0, Profiler.Snapshot.FrameCount);
    }

    [Fact]
    public void NestedScopesRecordDepthInBeginOrderWithAutoAndBeginEndMixed()
    {
        var token = Profiler.Acquire();
        Profiler.BeginFrame();
        using (Outer.Auto())
        {
            Inner.Begin();
            using (Leaf.Auto())
            {
            }

            Inner.End();
        }

        Profiler.EndFrame(2f, 0.5f);
        Profiler.Release(token);

        var frame = Newest();
        Assert.Equal(2f, frame.CpuMs);
        Assert.Equal(0.5f, frame.WaitMs);
        var events = EventsOf(frame);
        Assert.Equal(["Frame", "Test.Outer", "Test.Inner", "Test.Leaf"], events.Select(e => Profiler.MarkerAt(e.Marker).Name));
        Assert.Equal([0, 1, 2, 3], events.Select(e => (int)e.Depth));
        Assert.All(events, e => Assert.True(e.End >= e.Start));
        Assert.True(events[1].Start >= events[0].Start && events[1].End <= events[0].End);
        Assert.True(events[3].Start >= events[2].Start && events[3].End <= events[2].End);
        Assert.Equal(0, Profiler.UnbalancedScopes);
    }

    [Fact]
    public void UnbalancedScopesAreClosedAndCountedNeverThrown()
    {
        var token = Profiler.Acquire();
        Profiler.BeginFrame();
        var outer = Outer.Auto();
        Inner.Begin(); // never ended: the outer's end closes it
        outer.Dispose();
        Leaf.Begin(); // still open at the frame end
        Profiler.EndFrame(1f, 0f);
        Profiler.Release(token);

        Assert.Equal(2, Profiler.UnbalancedScopes);
        var events = EventsOf(Newest());
        Assert.Contains(events, e => e.Marker == Inner.Id);
        Assert.Contains(events, e => e.Marker == Leaf.Id);
        Assert.All(events, e => Assert.True(e.End >= e.Start));
    }

    [Fact]
    public void AcquireAndReleaseCountConsumersAndPauseFreezesHistory()
    {
        var a = Profiler.Acquire();
        var b = Profiler.Acquire();
        Assert.True(Profiler.IsRecording);
        Profiler.Release(a);
        Assert.True(Profiler.IsRecording);
        Profiler.Release(a); // twice: ignored
        Assert.Equal(1, Profiler.ConsumerCount);

        RunFrames(3);
        Assert.Equal(3, Profiler.Snapshot.FrameCount);
        Profiler.Paused = true;
        Assert.False(Profiler.IsRecording);
        RunFrames(5);
        Assert.Equal(3, Profiler.Snapshot.FrameCount);
        Profiler.Paused = false;
        RunFrames(1);
        Assert.Equal(4, Profiler.Snapshot.FrameCount);

        Profiler.Release(b);
        Assert.False(Profiler.IsRecording);
        Assert.Equal(0, Profiler.ConsumerCount);
    }

    [Fact]
    public void TheDisabledSwitchTurnsTheProfilerOff()
    {
        Assert.True(Profiler.ReadAvailability());
        AppContext.SetSwitch(Profiler.DisabledSwitch, true);
        try
        {
            Assert.False(Profiler.ReadAvailability());
        }
        finally
        {
            AppContext.SetSwitch(Profiler.DisabledSwitch, false);
        }
    }

    [Fact]
    public void RingWrapKeepsFrameTotalsAndLosesTheOldestEvents()
    {
        Profiler.ResetForTests(new ProfilerOptions { HistoryFrames = 50, EventCapacity = 64 });
        var token = Profiler.Acquire();
        for (var f = 0; f < 20; f++)
        {
            Profiler.BeginFrame();
            for (var i = 0; i < 10; i++)
                using (Leaf.Auto())
                {
                }

            Profiler.EndFrame(f, 0f);
        }

        Profiler.Release(token);
        var snapshot = Profiler.Snapshot;
        Assert.Equal(20, snapshot.FrameCount);
        Assert.Equal(0f, snapshot.GetFrame(0).CpuMs);
        Assert.Equal(11, snapshot.GetFrame(0).EventCount); // the total stays
        Assert.Equal(0, snapshot.GetEvents(snapshot.GetFrame(0)).Length); // the events are gone
        Assert.Equal(11, snapshot.GetEvents(snapshot.GetFrame(19)).Length);
    }

    [Fact]
    public void HistoryRingKeepsTheNewestFrames()
    {
        Profiler.ResetForTests(new ProfilerOptions { HistoryFrames = 4 });
        var token = Profiler.Acquire();
        RunFrames(10);
        Profiler.Release(token);
        var snapshot = Profiler.Snapshot;
        Assert.Equal(4, snapshot.FrameCount);
        Assert.Equal(10, snapshot.TotalFrames);
        Assert.Equal(snapshot.GetFrame(3).Number - 3, snapshot.GetFrame(0).Number);
    }

    [Fact]
    public void AFullThreadRingDropsScopesAndCountsThem()
    {
        var token = Profiler.Acquire();
        Profiler.BeginFrame();
        for (var i = 0; i < 40000; i++) // the main thread's staging ring holds 32768 per frame
            using (Leaf.Auto())
            {
            }

        Profiler.EndFrame(1f, 0f);
        Profiler.Release(token);
        Assert.True(Profiler.DroppedEvents > 0);
        Assert.Equal(32768, Newest().EventCount);
    }

    [Fact]
    public void BackgroundThreadsRecordOnceNamedAndLandInTheFrameTheyEndedIn()
    {
        var token = Profiler.Acquire();
        Profiler.BeginFrame();
        var unnamed = new Thread(() =>
        {
            using (Outer.Auto())
            {
            }
        });
        unnamed.Start();
        unnamed.Join();
        var named = new Thread(() =>
        {
            Profiler.SetThreadName("Worker");
            using (Inner.Auto())
                using (Leaf.Auto())
                {
                }
        });
        named.Start();
        named.Join();
        Profiler.EndFrame(1f, 0f);
        Profiler.Release(token);

        var events = EventsOf(Newest());
        Assert.DoesNotContain(events, e => e.Marker == Outer.Id); // unnamed threads are not recorded
        var worker = events.Where(e => e.Thread != 0).ToArray();
        Assert.Equal([Inner.Id, Leaf.Id], worker.Select(e => e.Marker));
        Assert.Equal([0, 1], worker.Select(e => (int)e.Depth));
        Assert.Equal("Worker", Profiler.ThreadName(worker[0].Thread));
    }

    [Fact]
    public void AReleasedLaneIsReusedByTheNextThreadOfTheSameName()
    {
        var token = Profiler.Acquire();
        byte? first = null;
        for (var i = 0; i < 3; i++)
        {
            Profiler.BeginFrame();
            var thread = new Thread(() =>
            {
                Profiler.SetThreadName("Loader");
                using (Leaf.Auto())
                {
                }

                Profiler.SetThreadName(null);
            });
            thread.Start();
            thread.Join();
            Profiler.EndFrame(1f, 0f);
            var lane = EventsOf(Newest()).Single(e => e.Thread != 0).Thread;
            first ??= lane;
            Assert.Equal(first, lane);
        }

        Profiler.Release(token);
    }

    [Fact]
    public void CountersKeepSetValuesAndRestartAddedOnesEachFrame()
    {
        var token = Profiler.Acquire();
        Profiler.BeginFrame();
        Enemies.Set(5);
        Hits.Add(2);
        Hits.Add(3);
        Profiler.EndFrame(1f, 0f);
        var first = Newest();
        Profiler.BeginFrame();
        Hits.Add(1);
        Profiler.EndFrame(1f, 0f);
        var second = Newest();
        Profiler.Release(token);

        var snapshot = Profiler.Snapshot;
        Assert.Equal(5, snapshot.GetCounter(first, Enemies));
        Assert.Equal(5, snapshot.GetCounter(second, Enemies));
        Assert.Equal(5, snapshot.GetCounter(first, Hits));
        Assert.Equal(1, snapshot.GetCounter(second, Hits));
        Assert.Equal("Test.Enemies", snapshot.GetCounterInfo(Enemies.Id).Name);
    }

    [Fact]
    public void GcCountersReportTheFramesMainThreadAllocations()
    {
        var token = Profiler.Acquire();
        RunFrames(2); // warm-up
        Profiler.BeginFrame();
        GC.KeepAlive(new byte[100_000]);
        Profiler.EndFrame(1f, 0f);
        Profiler.Release(token);
        Assert.InRange(Profiler.Snapshot.GetCounter(Newest(), EngineCounters.ManagedAllocated), 100_000, 200_000);
    }

    [Fact]
    public void SpikesSurviveTheRingWrapping()
    {
        Profiler.ResetForTests(new ProfilerOptions { HistoryFrames = 8, EventCapacity = 64, SpikeSlots = 2 });
        long captured = -1;
        Profiler.SpikeCaptured += n => captured = n;
        Profiler.SpikeThresholdMs = 10f;
        var token = Profiler.Acquire();
        Profiler.BeginFrame();
        using (Inner.Auto())
        {
        }

        Profiler.EndFrame(40f, 0f);
        var spike = Newest();
        Assert.True(spike.IsSpike);
        Assert.Equal(spike.Number, captured);
        for (var f = 0; f < 30; f++)
        {
            Profiler.BeginFrame();
            for (var i = 0; i < 4; i++)
                using (Leaf.Auto())
                {
                }

            Profiler.EndFrame(1f, 0f);
        }

        Profiler.Release(token);
        var snapshot = Profiler.Snapshot;
        Assert.Equal(1, snapshot.SpikeCount);
        var kept = snapshot.GetSpike(0);
        Assert.Equal(spike.Number, kept.Number);
        Assert.True(snapshot.TryFindFrame(spike.Number, out var found));
        Assert.Contains(EventsOf(found), e => e.Marker == Inner.Id);
    }

    [Fact]
    public void FreezeOnSpikeWaitsForTheFramesGpuData()
    {
        Profiler.SpikeThresholdMs = 10f;
        Profiler.FreezeOnSpike = true;
        var token = Profiler.Acquire();
        Profiler.BeginFrame();
        var spikeFrame = Profiler.FrameNumber;
        Profiler.ExpectGpu(spikeFrame);
        Profiler.EndFrame(50f, 0f);
        Assert.False(Profiler.Paused); // its GPU zones are still on the way

        Profiler.BeginFrame();
        Profiler.EndFrame(1f, 0f);
        Assert.False(Profiler.Paused);
        var now = Stopwatch.GetTimestamp();
        Profiler.SubmitGpu(spikeFrame, [new ProfilerEvent(now, now + 1000, EngineMarkers.GpuMainPass.Id, 0, ProfilerEvent.GpuThread)], 3f);
        Assert.True(Profiler.Paused);
        Assert.True(Profiler.Snapshot.TryFindFrame(spikeFrame, out var frame));
        Assert.Equal(3f, frame.GpuMs);
        Assert.False(frame.GpuPending);
        Assert.Equal(1, Profiler.Snapshot.GetGpuZones(frame).Length);
        Profiler.Release(token);
    }

    [Fact]
    public void LiteralScopesAreRegisteredOnce()
    {
        var token = Profiler.Acquire();
        var before = Profiler.MarkerCount;
        for (var f = 0; f < 3; f++)
        {
            Profiler.BeginFrame();
            using (Profiler.Scope("Test.Literal"))
            {
            }

            Profiler.EndFrame(1f, 0f);
        }

        Profiler.Release(token);
        Assert.Equal(before + 1, Profiler.MarkerCount);
        Assert.Contains(EventsOf(Newest()), e => Profiler.MarkerAt(e.Marker).Name == "Test.Literal");
    }

    [Fact]
    public void MarkersAtTheSameLineShareAnId()
    {
        var ids = new HashSet<ushort>();
        for (var i = 0; i < 3; i++)
            ids.Add(new ProfilerMarker("Test.Same").Id);
        Assert.Single(ids);
        Assert.NotEqual(0, ids.Single());
        var marker = Profiler.MarkerAt(ids.Single());
        Assert.EndsWith("ProfilerTests.cs", marker.File);
        Assert.True(marker.Line > 0);
    }

    [Fact]
    public void AllocationTrackingAttributesBytesToTheScope()
    {
        Profiler.ResetForTests(new ProfilerOptions { TrackAllocations = true });
        var token = Profiler.Acquire();
        Profiler.BeginFrame();
        using (Inner.Auto())
            GC.KeepAlive(new byte[50_000]);
        Profiler.EndFrame(1f, 0f);
        Profiler.Release(token);
        var inner = EventsOf(Newest()).Single(e => e.Marker == Inner.Id);
        Assert.InRange(inner.AllocatedBytes, 50_000, 100_000);
    }

    [Fact]
    public void StatisticsSplitSelfAndTotalTime()
    {
        var statistics = new ProfilerStatistics();
        ProfilerEvent[] events =
        [
            new(0, 100, 1, 0, 0),
            new(10, 40, 2, 1, 0),
            new(50, 70, 2, 1, 0),
            new(55, 60, 3, 2, 0),
            new(0, 30, 3, 0, 1), // another thread
        ];
        statistics.Add(events);
        Assert.Equal(100, statistics.TotalTicks[1]);
        Assert.Equal(50, statistics.SelfTicks[1]);
        Assert.Equal(50, statistics.TotalTicks[2]);
        Assert.Equal(45, statistics.SelfTicks[2]);
        Assert.Equal(2, statistics.Calls[2]);
        Assert.Equal(30, statistics.MaxTicks[2]);
        Assert.Equal(35, statistics.SelfTicks[3]);
        Span<ushort> top = stackalloc ushort[2];
        Assert.Equal(2, statistics.Top(statistics.SelfTicks, top));
        Assert.Equal(1, top[0]);
        Assert.Equal(2, top[1]);
        statistics.Clear();
        Assert.Equal(0, statistics.TotalTicks[1]);
        Assert.True(statistics.Markers.IsEmpty);
    }

    internal static void RunFrames(int count)
    {
        for (var f = 0; f < count; f++)
        {
            Profiler.BeginFrame();
            using (Leaf.Auto())
            {
            }

            Profiler.EndFrame(1f, 0f);
        }
    }
}
