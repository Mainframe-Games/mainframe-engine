namespace MainframeEngine.Networking;

/// <summary>
/// The last <see cref="Capacity"/> received values of one interpolated member, by server tick (a ring buffer;
/// allocation-free after construction). <see cref="Sample"/> returns the two values bracketing the render tick and the
/// blend factor; generated code blends them with <see cref="NetLerp"/>.
/// </summary>
/// <remarks>
/// Snapshots only carry changed members, so a member absent from a snapshot kept its value through that tick.
/// <see cref="Push"/> therefore inserts a "hold" sample at the previous snapshot tick before a new value that follows
/// a pause, so motion resuming after standing still blends from the right moment instead of from when it stopped.
/// </remarks>
public sealed class InterpolationBuffer<T>
{
    /// <summary>Samples kept (about one second at 30 Hz).</summary>
    public const int Capacity = 32;

    private readonly uint[] _ticks = new uint[Capacity];
    private readonly T[] _values = new T[Capacity];
    private int _start;
    private int _count;

    /// <summary>Samples currently buffered.</summary>
    public int Count => _count;

    /// <summary>The newest sample's tick (0 when empty).</summary>
    public uint NewestTick => _count == 0 ? 0 : TickAt(_count - 1);

    public void Clear()
    {
        _start = 0;
        _count = 0;
        Array.Clear(_values);
    }

    /// <summary>Adds the value received for <paramref name="tick"/>; older or duplicate ticks are ignored.</summary>
    /// <param name="tick">Server tick of the snapshot.</param>
    /// <param name="value">The member's value at that tick.</param>
    /// <param name="previousTick">The previous snapshot applied by the client (the member was unchanged through it).</param>
    public void Push(uint tick, T value, uint previousTick)
    {
        if (_count > 0)
        {
            var newest = TickAt(_count - 1);
            if (tick <= newest)
            {
                if (tick == newest)
                    _values[Index(_count - 1)] = value;
                return;
            }

            if (newest < previousTick && previousTick < tick)
                Add(previousTick, _values[Index(_count - 1)]);
        }

        Add(tick, value);
    }

    /// <summary>
    /// The values to blend at <paramref name="time"/>: <c>Lerp(from, to, t)</c>. <paramref name="t"/> is in [0, 1]
    /// between samples and above 1 when extrapolating past the newest sample (at most
    /// <see cref="InterpolationTime.MaxExtrapolationTicks"/>). False when the buffer is empty.
    /// </summary>
    public bool Sample(in InterpolationTime time, out T from, out T to, out float t)
    {
        if (_count == 0)
        {
            from = default!;
            to = default!;
            t = 0;
            return false;
        }

        var render = time.RenderTick;
        var last = _count - 1;
        var newestTick = TickAt(last);

        if (render >= newestTick)
        {
            // Past the newest sample. If a later snapshot was applied without this member, it is known to be still:
            // hold. Otherwise the data is late: keep moving along the last velocity, briefly.
            if (_count < 2 || time.LatestTick > newestTick)
            {
                from = to = _values[Index(last)];
                t = 0;
                return true;
            }

            var previousTick = TickAt(last - 1);
            var span = (double)(newestTick - previousTick);
            var ahead = Math.Min(render - newestTick, Math.Max(0, time.MaxExtrapolationTicks));
            from = _values[Index(last - 1)];
            to = _values[Index(last)];
            t = (float)(1 + ahead / span);
            return true;
        }

        if (render <= TickAt(0))
        {
            from = to = _values[Index(0)];
            t = 0;
            return true;
        }

        // Newest first: the render tick is usually within the last couple of samples.
        for (var i = last - 1; i >= 0; i--)
        {
            var a = TickAt(i);
            if (a > render)
                continue;
            var b = TickAt(i + 1);
            from = _values[Index(i)];
            to = _values[Index(i + 1)];
            t = (float)((render - a) / (b - a));
            return true;
        }

        from = to = _values[Index(0)];
        t = 0;
        return true;
    }

    private void Add(uint tick, T value)
    {
        if (_count == Capacity)
        {
            _start = (_start + 1) % Capacity;
            _count--;
        }

        var index = Index(_count);
        _ticks[index] = tick;
        _values[index] = value;
        _count++;
    }

    private int Index(int i) => (_start + i) % Capacity;

    private uint TickAt(int i) => _ticks[Index(i)];
}
