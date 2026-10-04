namespace MainframeEngine;

/// <summary>
/// Ring-buffer bookkeeping for the upload staging buffer: allocations are carved from the head in order and
/// tagged with the frame whose command buffer copies them; they are released once that frame has finished.
/// Pure logic (no Vulkan), unit-tested directly.
/// </summary>
/// <remarks>
/// Tags never decrease, so live allocations always form one contiguous span from the tail to the head
/// (possibly wrapping) and release is a FIFO pop. When neither the space after the head nor the space before
/// the tail fits a request, <see cref="TryAllocate"/> fails and the caller falls back to a temporary staging
/// buffer. Allocation-free once the entry queue has grown.
/// </remarks>
internal sealed class StagingRing
{
    // Start = head before the allocation (so alignment and wrap padding are owned by the entry), End = new head.
    private readonly Queue<(ulong Frame, ulong End)> _live = new(32);
    private ulong _head;
    private ulong _tail;
    private ulong _lastFrame;

    public StagingRing(ulong capacity)
    {
        ArgumentOutOfRangeException.ThrowIfZero(capacity);
        Capacity = capacity;
    }

    public ulong Capacity { get; }

    public int LiveAllocations => _live.Count;

    /// <summary>Bytes held by live allocations, including alignment and wrap padding.</summary>
    public ulong UsedBytes => _live.Count == 0 ? 0 : _head > _tail ? _head - _tail : Capacity - _tail + _head;

    /// <summary>Reserves <paramref name="size"/> bytes aligned to <paramref name="alignment"/> for <paramref name="frame"/>.</summary>
    public bool TryAllocate(ulong size, ulong alignment, ulong frame, out ulong offset)
    {
        ArgumentOutOfRangeException.ThrowIfZero(size);
        if (alignment == 0) alignment = 1;
        if ((alignment & (alignment - 1)) != 0)
            throw new ArgumentException($"Alignment {alignment} is not a power of two.", nameof(alignment));
        if (frame < _lastFrame)
            throw new ArgumentOutOfRangeException(nameof(frame), "Allocation frames must not decrease.");

        offset = 0;
        if (size > Capacity)
            return false;

        ulong start;
        if (_live.Count == 0 || _head > _tail)
        {
            // Free: [head, capacity) then, after a wrap, [0, tail).
            start = FreeListBlock.AlignUp(_head, alignment);
            if (start > Capacity || Capacity - start < size)
            {
                start = 0;
                if (_live.Count > 0 && size > _tail)
                    return false;
            }
        }
        else
        {
            // Wrapped (or full when head == tail): free is [head, tail).
            start = FreeListBlock.AlignUp(_head, alignment);
            if (start > _tail || _tail - start < size)
                return false;
        }

        var end = start + size;
        _live.Enqueue((frame, end));
        _head = end == Capacity ? 0 : end;
        _lastFrame = frame;
        offset = start;
        return true;
    }

    /// <summary>Releases every allocation whose frame has finished.</summary>
    public void Release(ulong completedFrame)
    {
        while (_live.TryPeek(out var entry) && entry.Frame <= completedFrame)
        {
            _live.Dequeue();
            _tail = entry.End == Capacity ? 0 : entry.End;
        }

        if (_live.Count == 0)
            _head = _tail = 0;
    }
}
