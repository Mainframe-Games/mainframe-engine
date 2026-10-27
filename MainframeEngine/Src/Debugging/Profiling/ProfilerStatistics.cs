namespace MainframeEngine;

/// <summary>Per-marker totals over a set of events: calls, total and self time (total minus children), maximum, allocations.</summary>
/// <remarks>
/// Arrays are indexed by marker id and sized <see cref="Profiler.MaxMarkers"/>; reuse one instance (the overlay and the
/// editor do), <see cref="Clear"/> between windows. Adding events allocates nothing.
/// </remarks>
public sealed class ProfilerStatistics
{
    private readonly int[] _stackIndex = new int[ProfilerThreadBuffer.MaxDepth + 1];
    private readonly long[] _stackChildren = new long[ProfilerThreadBuffer.MaxDepth + 1];
    private readonly byte[] _stackDepth = new byte[ProfilerThreadBuffer.MaxDepth + 1];
    private readonly ushort[] _stackMarker = new ushort[ProfilerThreadBuffer.MaxDepth + 1];
    private readonly long[] _stackDuration = new long[ProfilerThreadBuffer.MaxDepth + 1];
    private readonly ushort[] _touched = new ushort[Profiler.MaxMarkers];
    private readonly bool[] _isTouched = new bool[Profiler.MaxMarkers];
    private int _touchedCount;

    public int[] Calls { get; } = new int[Profiler.MaxMarkers];

    /// <summary>Total ticks per marker (nested calls of the same marker count twice).</summary>
    public long[] TotalTicks { get; } = new long[Profiler.MaxMarkers];

    /// <summary>Ticks per marker not spent in child scopes.</summary>
    public long[] SelfTicks { get; } = new long[Profiler.MaxMarkers];

    /// <summary>Longest single call per marker.</summary>
    public long[] MaxTicks { get; } = new long[Profiler.MaxMarkers];

    public long[] AllocatedBytes { get; } = new long[Profiler.MaxMarkers];

    /// <summary>Markers seen since the last <see cref="Clear"/>, in first-seen order.</summary>
    public ReadOnlySpan<ushort> Markers => _touched.AsSpan(0, _touchedCount);

    /// <summary>Frames added since the last clear (callers divide by it for per-frame averages).</summary>
    public int Frames { get; set; }

    public void Clear()
    {
        foreach (var marker in Markers)
        {
            Calls[marker] = 0;
            TotalTicks[marker] = 0;
            SelfTicks[marker] = 0;
            MaxTicks[marker] = 0;
            AllocatedBytes[marker] = 0;
            _isTouched[marker] = false;
        }

        _touchedCount = 0;
        Frames = 0;
    }

    /// <summary>Adds <paramref name="events"/> (each thread's events in begin order, as the profiler stores them).</summary>
    public void Add(ProfilerEventSpans events)
    {
        Add(events.First);
        Add(events.Second);
    }

    /// <summary>Adds <paramref name="events"/> (begin order per thread; threads may follow one another).</summary>
    public void Add(ReadOnlySpan<ProfilerEvent> events)
    {
        var top = 0;
        var thread = -1;
        foreach (ref readonly var e in events)
        {
            if (e.Thread != thread)
            {
                while (top > 0)
                    Pop(ref top);
                thread = e.Thread;
            }

            while (top > 0 && _stackDepth[top - 1] >= e.Depth)
                Pop(ref top);
            var duration = Math.Max(0, e.DurationTicks);
            if (top > 0)
                _stackChildren[top - 1] += duration;
            if (top < _stackIndex.Length)
            {
                _stackDepth[top] = e.Depth;
                _stackMarker[top] = e.Marker;
                _stackDuration[top] = duration;
                _stackChildren[top] = 0;
                top++;
            }

            var m = e.Marker;
            if (!_isTouched[m])
            {
                _isTouched[m] = true;
                _touched[_touchedCount++] = m;
            }

            Calls[m]++;
            TotalTicks[m] += duration;
            if (duration > MaxTicks[m])
                MaxTicks[m] = duration;
            AllocatedBytes[m] += e.AllocatedBytes;
        }

        while (top > 0)
            Pop(ref top);
    }

    private void Pop(ref int top)
    {
        top--;
        SelfTicks[_stackMarker[top]] += Math.Max(0, _stackDuration[top] - _stackChildren[top]);
    }

    /// <summary>
    /// Fills <paramref name="into"/> with the markers of the largest <paramref name="key"/> values (descending); returns how
    /// many. A partial selection: no sort of the whole set, no allocation.
    /// </summary>
    public int Top(long[] key, Span<ushort> into)
    {
        ArgumentNullException.ThrowIfNull(key);
        var count = 0;
        foreach (var marker in Markers)
        {
            var value = key[marker];
            var at = count;
            while (at > 0 && key[into[at - 1]] < value)
                at--;
            if (at >= into.Length)
                continue;
            var end = Math.Min(count, into.Length - 1);
            for (var i = end; i > at; i--)
                into[i] = into[i - 1];
            into[at] = marker;
            if (count < into.Length)
                count++;
        }

        return count;
    }
}
