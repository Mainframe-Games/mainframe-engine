using System.Diagnostics;

namespace MainframeEngine;

/// <summary>
/// One thread's scope recorder: a single-producer ring of events in begin order (a slot is reserved when a scope begins
/// and written when it ends) and the stack of open scopes. Events become visible to the main thread, which drains every
/// buffer at the frame end, once the thread's outermost scope has ended. Producer methods are wait-free and never
/// allocate; a full ring drops the scope and counts it.
/// </summary>
internal sealed class ProfilerThreadBuffer
{
    /// <summary>Open scopes per thread at most (events store the depth as a byte).</summary>
    public const int MaxDepth = 255;

    private readonly ProfilerEvent[] _ring;
    private readonly int _mask;
    private long _write;     // producer: next slot to reserve
    private long _published; // producer → consumer: every slot below is written
    private long _read;      // consumer → producer: every slot below has been drained

    private readonly long[] _openSlot = new long[MaxDepth];    // ring position, -1 when the scope was dropped
    private readonly ushort[] _openMarker = new ushort[MaxDepth];
    private readonly long[] _openStart = new long[MaxDepth];
    private readonly long[] _openAlloc = new long[MaxDepth];
    private readonly int[] _openSerial = new int[MaxDepth];
    private int _depth;
    private int _serial;

    private long _dropped;
    private int _unbalanced;
    private int _lastUnbalancedMarker = -1;

    public ProfilerThreadBuffer(int generation, byte index, string name, int capacity)
    {
        if (capacity < 16 || (capacity & (capacity - 1)) != 0)
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "The capacity must be a power of two of at least 16.");
        Generation = generation;
        Index = index;
        Name = name;
        _ring = new ProfilerEvent[capacity];
        _mask = capacity - 1;
    }

    public int Generation { get; }

    /// <summary>The thread lane (0 = main).</summary>
    public byte Index { get; }

    public string Name { get; set; }

    public int Capacity => _ring.Length;

    private int _free;

    /// <summary>
    /// Released by its thread (<see cref="Profiler.SetThreadName"/> null): another thread of the same name may take it over
    /// (a pool thread running the next load), so lanes do not pile up.
    /// </summary>
    public bool IsFree => Volatile.Read(ref _free) != 0;

    public void MarkFree() => Volatile.Write(ref _free, 1);

    /// <summary>Takes a free buffer over for the calling thread; false when another thread got it first.</summary>
    public bool TryClaim() => Interlocked.CompareExchange(ref _free, 0, 1) == 1;

    /// <summary>Open scopes right now (producer thread).</summary>
    public int Depth => _depth;

    /// <summary>Scopes dropped because the ring or the depth stack was full (total; read from any thread).</summary>
    public long Dropped => Volatile.Read(ref _dropped);

    /// <summary>Scopes closed out of order or by the frame end (total).</summary>
    public int Unbalanced => Volatile.Read(ref _unbalanced);

    /// <summary>The marker of the latest unbalanced scope (-1: none yet).</summary>
    public int LastUnbalancedMarker => Volatile.Read(ref _lastUnbalancedMarker);

    // ── Producer ─────────────────────────────────────────────────────────────────────────────────────────────

    public ProfilerScope Begin(ushort marker, bool trackAllocations)
    {
        var depth = _depth;
        if (depth >= MaxDepth)
        {
            Volatile.Write(ref _dropped, _dropped + 1);
            return default;
        }

        long slot;
        if (_write - Volatile.Read(ref _read) >= _ring.Length)
        {
            slot = -1; // full: the scope still nests (its children keep their depth) but records nothing
            Volatile.Write(ref _dropped, _dropped + 1);
        }
        else
        {
            slot = _write++;
        }

        var serial = ++_serial;
        _openSlot[depth] = slot;
        _openMarker[depth] = marker;
        _openSerial[depth] = serial;
        _openAlloc[depth] = trackAllocations ? GC.GetAllocatedBytesForCurrentThread() : 0;
        _depth = depth + 1;
        _openStart[depth] = Stopwatch.GetTimestamp(); // last: the scope's own bookkeeping is not in its time
        return new ProfilerScope(this, depth, serial);
    }

    /// <summary>Ends the scope begun at stack position <paramref name="depth"/> (stale tokens, already closed, do nothing).</summary>
    public void End(int depth, int serial)
    {
        var now = Stopwatch.GetTimestamp();
        if (depth >= _depth || _openSerial[depth] != serial)
            return; // closed already (out of order, or by the frame end)
        while (_depth - 1 > depth)
            Close(now, unbalanced: true);
        Close(now, unbalanced: false);
    }

    /// <summary>Ends <paramref name="marker"/>'s innermost open scope; false when none is open.</summary>
    public bool EndMarker(ushort marker)
    {
        var now = Stopwatch.GetTimestamp();
        for (var d = _depth - 1; d >= 0; d--)
        {
            if (_openMarker[d] != marker)
                continue;
            while (_depth - 1 > d)
                Close(now, unbalanced: true);
            Close(now, unbalanced: false);
            return true;
        }

        CountUnbalanced(marker);
        return false;
    }

    /// <summary>Closes every open scope at <paramref name="now"/>, counting each (a frame ended with them open).</summary>
    public void CloseAll(long now)
    {
        while (_depth > 0)
            Close(now, unbalanced: true);
    }

    private void Close(long now, bool unbalanced)
    {
        var depth = --_depth;
        var slot = _openSlot[depth];
        var marker = _openMarker[depth];
        _openSerial[depth] = 0;
        if (unbalanced)
            CountUnbalanced(marker);
        if (slot >= 0)
        {
            var allocStart = _openAlloc[depth];
            var allocated = allocStart == 0 ? 0 : (int)Math.Min(int.MaxValue, GC.GetAllocatedBytesForCurrentThread() - allocStart);
            _ring[slot & _mask] = new ProfilerEvent(_openStart[depth], now, marker, (byte)depth, Index, allocated);
        }

        if (depth == 0)
            Volatile.Write(ref _published, _write);
    }

    private void CountUnbalanced(ushort marker)
    {
        Volatile.Write(ref _lastUnbalancedMarker, marker);
        Volatile.Write(ref _unbalanced, _unbalanced + 1);
    }

    // ── Consumer (main thread) ───────────────────────────────────────────────────────────────────────────────

    /// <summary>Events published since the last drain, as up to two spans of the ring; call <see cref="CompleteDrain"/> after copying them.</summary>
    public long Peek(out ReadOnlySpan<ProfilerEvent> first, out ReadOnlySpan<ProfilerEvent> second)
    {
        var read = _read;
        var published = Volatile.Read(ref _published);
        var count = (int)(published - read);
        var start = (int)(read & _mask);
        var head = Math.Min(count, _ring.Length - start);
        first = new ReadOnlySpan<ProfilerEvent>(_ring, start, head);
        second = new ReadOnlySpan<ProfilerEvent>(_ring, 0, count - head);
        return published;
    }

    public void CompleteDrain(long published) => Volatile.Write(ref _read, published);
}
