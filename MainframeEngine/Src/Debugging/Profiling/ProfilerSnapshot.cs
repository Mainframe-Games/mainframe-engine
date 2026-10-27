namespace MainframeEngine;

/// <summary>
/// Read access to the profiler's history (<see cref="Profiler.Snapshot"/>): frames oldest first, their CPU events, GPU
/// zones and counters, the spike frames, and the marker and counter registries. Main thread only (the frame end writes
/// the history there); nothing here allocates.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static",
    Justification = "An instance API over the static recorder, so callers can pass the snapshot around.")]
public sealed class ProfilerSnapshot
{
    internal ProfilerSnapshot()
    {
    }

    /// <summary>Frames in the history (at most <see cref="ProfilerOptions.HistoryFrames"/>).</summary>
    public int FrameCount => Profiler.History?.FrameCount ?? 0;

    /// <summary>Frames recorded since the history was created (the ring keeps the newest <see cref="FrameCount"/>).</summary>
    public long TotalFrames => Profiler.History?.FramesWritten ?? 0;

    /// <summary>Frame <paramref name="index"/>, 0 = the oldest kept.</summary>
    public ProfilerFrame GetFrame(int index)
    {
        var history = Profiler.History;
        if (history is null || (uint)index >= (uint)history.FrameCount)
            throw new ArgumentOutOfRangeException(nameof(index), index, "No such frame in the history.");
        return new ProfilerFrame(history.FrameAt(history.FramesWritten - history.FrameCount + index));
    }

    /// <summary>The newest frame; false when nothing was recorded.</summary>
    public bool TryGetNewest(out ProfilerFrame frame)
    {
        frame = default;
        if (Profiler.History is not { FrameCount: > 0 } history)
            return false;
        frame = new ProfilerFrame(history.FrameAt(history.FramesWritten - 1));
        return true;
    }

    /// <summary>The frame numbered <paramref name="number"/>, if still in the history (or kept as a spike).</summary>
    public bool TryFindFrame(long number, out ProfilerFrame frame)
    {
        frame = default;
        if (Profiler.History is not { } history)
            return false;
        var sequence = history.FindSequence(number, history.FrameCount);
        if (sequence >= 0)
        {
            frame = new ProfilerFrame(history.FrameAt(sequence));
            return true;
        }

        foreach (var slot in history.Spikes)
        {
            if (slot.Used && slot.Frame.Number == number)
            {
                frame = new ProfilerFrame(slot.Frame);
                return true;
            }
        }

        return false;
    }

    /// <summary>The CPU events of <paramref name="frame"/> still in the ring (empty once it wrapped past them; spikes keep theirs).</summary>
    public ProfilerEventSpans GetEvents(in ProfilerFrame frame)
    {
        if (Profiler.History is not { } history)
            return default;
        var events = history.EventsOf(frame.Record);
        if (events.Length == frame.EventCount || SpikeOf(history, frame.Number) is not { } slot)
            return events;
        return new ProfilerEventSpans(new ReadOnlySpan<ProfilerEvent>(slot.Events, 0, slot.EventCount), default);
    }

    /// <summary>The GPU zones of <paramref name="frame"/> (aligned to the CPU clock).</summary>
    public ProfilerEventSpans GetGpuZones(in ProfilerFrame frame)
    {
        if (Profiler.History is not { } history)
            return default;
        var zones = history.GpuOf(frame.Record);
        if (zones.Length == frame.GpuZoneCount || SpikeOf(history, frame.Number) is not { } slot)
            return zones;
        return new ProfilerEventSpans(new ReadOnlySpan<ProfilerEvent>(slot.Gpu, 0, slot.GpuCount), default);
    }

    /// <summary>Counter <paramref name="counter"/>'s value in <paramref name="frame"/> (0 when not recorded).</summary>
    public long GetCounter(in ProfilerFrame frame, ushort counter)
    {
        if (Profiler.History is not { } history || counter >= history.CounterCapacity)
            return 0;
        var sequence = frame.Record.Sequence;
        if (history.FindSequence(frame.Number, history.FrameCount) == sequence)
            return history.CountersOf(sequence)[counter];
        return SpikeOf(history, frame.Number) is { } slot ? slot.Counters[counter] : 0;
    }

    public long GetCounter(in ProfilerFrame frame, ProfilerCounter counter) => GetCounter(frame, counter.Id);

    /// <summary>Spike frames kept (newest last), at most <see cref="ProfilerOptions.SpikeSlots"/>.</summary>
    public int SpikeCount
    {
        get
        {
            if (Profiler.History is not { } history)
                return 0;
            var count = 0;
            foreach (var slot in history.Spikes)
                if (slot.Used)
                    count++;
            return count;
        }
    }

    /// <summary>Spike <paramref name="index"/> in capture order (0 = the oldest kept).</summary>
    public ProfilerFrame GetSpike(int index)
    {
        if (Profiler.History is not { } history)
            throw new ArgumentOutOfRangeException(nameof(index));
        // Selection by order without sorting: the index-th smallest capture order among the used slots.
        long last = 0;
        for (var i = 0; i <= index; i++)
        {
            ProfilerSpikeSlot? next = null;
            foreach (var slot in history.Spikes)
                if (slot.Used && slot.CapturedOrder > last && (next is null || slot.CapturedOrder < next.CapturedOrder))
                    next = slot;
            if (next is null)
                throw new ArgumentOutOfRangeException(nameof(index), index, "No such spike.");
            if (i == index)
                return new ProfilerFrame(next.Frame);
            last = next.CapturedOrder;
        }

        throw new ArgumentOutOfRangeException(nameof(index));
    }

    private static ProfilerSpikeSlot? SpikeOf(ProfilerHistory history, long number)
    {
        foreach (var slot in history.Spikes)
            if (slot.Used && slot.Frame.Number == number)
                return slot;
        return null;
    }

    public int MarkerCount => Profiler.MarkerCount;

    public ProfilerMarkerInfo GetMarker(ushort id) => Profiler.MarkerAt(id);

    public int CounterCount => Profiler.CounterCount;

    public ProfilerCounterInfo GetCounterInfo(ushort id) => Profiler.CounterAt(id);

    /// <summary>Thread lane <paramref name="thread"/>'s name (null when unused).</summary>
    public string? GetThreadName(byte thread) => Profiler.ThreadName(thread);

    /// <summary>Changes whenever the history does.</summary>
    public int Version => Profiler.Version;

    public long DroppedEvents => Profiler.DroppedEvents;

    public long UnbalancedScopes => Profiler.UnbalancedScopes;
}
