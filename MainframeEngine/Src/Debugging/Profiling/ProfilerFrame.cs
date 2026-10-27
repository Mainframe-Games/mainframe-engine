namespace MainframeEngine;

/// <summary>
/// One recorded frame: its number, start and end (<see cref="System.Diagnostics.Stopwatch"/> ticks), CPU time without the
/// GPU/swapchain wait, that wait, the GPU time (first to last timestamp; NaN until read back, two frames later, or when
/// untimed) and whether it was a spike. Its events, GPU zones and counters are read through <see cref="ProfilerSnapshot"/>.
/// </summary>
public readonly struct ProfilerFrame
{
    internal ProfilerFrame(in ProfilerFrameRecord record) => Record = record;

    internal ProfilerFrameRecord Record { get; }

    public long Number => Record.Number;

    public long StartTicks => Record.StartTicks;

    public long EndTicks => Record.EndTicks;

    /// <summary>Wall-clock time from this frame's start to its end, in milliseconds.</summary>
    public double DurationMs => (EndTicks - StartTicks) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

    /// <summary>CPU time (update + render) without <see cref="WaitMs"/>.</summary>
    public float CpuMs => Record.CpuMs;

    /// <summary>Time blocked on the GPU or the swapchain (fence, acquire, present).</summary>
    public float WaitMs => Record.WaitMs;

    /// <summary>GPU time, first to last timestamp; NaN while pending or untimed.</summary>
    public float GpuMs => Record.GpuMs;

    /// <summary>True while the frame's GPU zones have not been read back yet.</summary>
    public bool GpuPending => Record.GpuPending;

    public bool IsSpike => Record.IsSpike;

    /// <summary>CPU events of the frame (still in the ring: see <see cref="ProfilerSnapshot.GetEvents"/>).</summary>
    public int EventCount => Record.EventCount;

    public int GpuZoneCount => Record.GpuCount;
}

/// <summary>Storage form of a frame (the ring's element).</summary>
internal struct ProfilerFrameRecord
{
    public long Number;
    public long Sequence;
    public long StartTicks;
    public long EndTicks;
    public float CpuMs;
    public float WaitMs;
    public float GpuMs;
    public bool GpuPending;
    public bool IsSpike;
    public long EventFirst;
    public int EventCount;
    public long GpuFirst;
    public int GpuCount;
}

/// <summary>Two consecutive pieces of a ring (the second is empty unless the range wraps).</summary>
public readonly ref struct ProfilerEventSpans(ReadOnlySpan<ProfilerEvent> first, ReadOnlySpan<ProfilerEvent> second)
{
    public ReadOnlySpan<ProfilerEvent> First { get; } = first;

    public ReadOnlySpan<ProfilerEvent> Second { get; } = second;

    public int Length => First.Length + Second.Length;

    public ProfilerEvent this[int index] => index < First.Length ? First[index] : Second[index - First.Length];

    /// <summary>Copies both pieces into <paramref name="destination"/> (at most its length); returns how many.</summary>
    public int CopyTo(Span<ProfilerEvent> destination)
    {
        var a = Math.Min(First.Length, destination.Length);
        First[..a].CopyTo(destination);
        var b = Math.Min(Second.Length, destination.Length - a);
        Second[..b].CopyTo(destination[a..]);
        return a + b;
    }
}

/// <summary>A spike kept apart from the ring: the frame, copies of its events and GPU zones, its counters.</summary>
internal sealed class ProfilerSpikeSlot(int eventCapacity, int gpuCapacity, int counterCapacity)
{
    public ProfilerFrameRecord Frame;
    public readonly ProfilerEvent[] Events = new ProfilerEvent[eventCapacity];
    public int EventCount;
    public readonly ProfilerEvent[] Gpu = new ProfilerEvent[gpuCapacity];
    public int GpuCount;
    public readonly long[] Counters = new long[counterCapacity];
    public bool Used;
    public long CapturedOrder;
}

/// <summary>The preallocated history: frame ring, CPU event ring, GPU event ring, counter block and spike slots.</summary>
internal sealed class ProfilerHistory
{
    public ProfilerHistory(ProfilerOptions options, int counterCapacity, int spikeEventCapacity)
    {
        Options = options;
        Frames = new ProfilerFrameRecord[options.HistoryFrames];
        Events = new ProfilerEvent[options.EventCapacity];
        Gpu = new ProfilerEvent[Math.Max(64, options.HistoryFrames * Math.Min(options.MaxGpuZones, 32))];
        CounterCapacity = counterCapacity;
        Counters = new long[options.HistoryFrames * counterCapacity];
        Spikes = new ProfilerSpikeSlot[options.SpikeSlots];
        for (var i = 0; i < Spikes.Length; i++)
            Spikes[i] = new ProfilerSpikeSlot(spikeEventCapacity, options.MaxGpuZones, counterCapacity);
    }

    public ProfilerOptions Options { get; }

    public readonly ProfilerFrameRecord[] Frames;
    public long FramesWritten;

    public readonly ProfilerEvent[] Events;
    public long EventsWritten;

    public readonly ProfilerEvent[] Gpu;
    public long GpuWritten;

    public readonly int CounterCapacity;
    public readonly long[] Counters;

    public readonly ProfilerSpikeSlot[] Spikes;
    public long SpikeOrder;

    public int FrameCount => (int)Math.Min(FramesWritten, Frames.Length);

    public ref ProfilerFrameRecord FrameAt(long sequence) => ref Frames[sequence % Frames.Length];

    /// <summary>The frame with <paramref name="number"/> among the newest <paramref name="lookBack"/>, or -1.</summary>
    public long FindSequence(long number, int lookBack)
    {
        var oldest = Math.Max(FramesWritten - FrameCount, FramesWritten - lookBack);
        for (var s = FramesWritten - 1; s >= oldest; s--)
            if (FrameAt(s).Number == number)
                return s;
        return -1;
    }

    public void AppendEvents(ReadOnlySpan<ProfilerEvent> events)
    {
        var capacity = Events.Length;
        if (events.Length > capacity)
            events = events[^capacity..];
        var start = (int)(EventsWritten % capacity);
        var head = Math.Min(events.Length, capacity - start);
        events[..head].CopyTo(Events.AsSpan(start));
        events[head..].CopyTo(Events);
        EventsWritten += events.Length;
    }

    public void AppendGpu(ReadOnlySpan<ProfilerEvent> events)
    {
        var capacity = Gpu.Length;
        if (events.Length > capacity)
            events = events[^capacity..];
        var start = (int)(GpuWritten % capacity);
        var head = Math.Min(events.Length, capacity - start);
        events[..head].CopyTo(Gpu.AsSpan(start));
        events[head..].CopyTo(Gpu);
        GpuWritten += events.Length;
    }

    public ProfilerEventSpans EventsOf(in ProfilerFrameRecord frame) =>
        Slice(Events, EventsWritten, frame.EventFirst, frame.EventCount);

    public ProfilerEventSpans GpuOf(in ProfilerFrameRecord frame) =>
        Slice(Gpu, GpuWritten, frame.GpuFirst, frame.GpuCount);

    // The part of [first, first + count) still in a ring that has seen `written` elements (the oldest are overwritten).
    private static ProfilerEventSpans Slice(ProfilerEvent[] ring, long written, long first, int count)
    {
        var oldest = Math.Max(0, written - ring.Length);
        var end = first + count;
        if (end <= oldest || count == 0)
            return default;
        first = Math.Max(first, oldest);
        var length = (int)(end - first);
        var start = (int)(first % ring.Length);
        var head = Math.Min(length, ring.Length - start);
        return new ProfilerEventSpans(new ReadOnlySpan<ProfilerEvent>(ring, start, head), new ReadOnlySpan<ProfilerEvent>(ring, 0, length - head));
    }

    public Span<long> CountersOf(long sequence) => Counters.AsSpan((int)(sequence % Frames.Length) * CounterCapacity, CounterCapacity);
}
