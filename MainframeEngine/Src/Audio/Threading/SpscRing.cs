using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace MainframeEngine.Audio;

/// <summary>
/// A bounded, lock-free, single-producer / single-consumer ring of value types (the audio command and event
/// queues). One thread only enqueues, one other thread only dequeues; neither ever blocks or allocates.
/// </summary>
/// <remarks>
/// <para>The producer owns <c>_tail</c>, the consumer owns <c>_head</c>; each publishes its index with a release
/// write and reads the other's with an acquire read, so an item is fully written before the consumer can see it
/// and fully read before the producer may overwrite it. Indices are monotonically increasing 64-bit counters
/// (wrap-free in practice) masked into a power-of-two array. Both indices sit on their own cache line so the two
/// threads do not false-share.</para>
/// <para><see cref="EnqueueBatch"/> writes several items and publishes them with a single tail store, so the
/// consumer sees a frame's commands all at once or not at all.</para>
/// </remarks>
internal sealed class SpscRing<T> where T : struct
{
    private readonly T[] _items;
    private readonly long _mask;
    private PaddedIndex _head; // next slot to read (consumer)
    private PaddedIndex _tail; // next slot to write (producer)

    /// <param name="capacity">Rounded up to a power of two (minimum 2).</param>
    public SpscRing(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        var size = (int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(2, capacity));
        _items = new T[size];
        _mask = size - 1;
    }

    public int Capacity => _items.Length;

    /// <summary>Items currently queued (a snapshot; exact only from the producer or the consumer thread).</summary>
    public int Count => (int)(Volatile.Read(ref _tail.Value) - Volatile.Read(ref _head.Value));

    /// <summary>Free slots as seen by the producer.</summary>
    public int FreeSpace => Capacity - Count;

    /// <summary>Producer: queues <paramref name="item"/>; false when the ring is full.</summary>
    public bool TryEnqueue(in T item)
    {
        var tail = _tail.Value;
        if (tail - Volatile.Read(ref _head.Value) >= _items.Length)
            return false;
        _items[tail & _mask] = item;
        Volatile.Write(ref _tail.Value, tail + 1);
        return true;
    }

    /// <summary>
    /// Producer: queues as many of <paramref name="items"/> as fit (in order) and publishes them together.
    /// Returns how many were queued.
    /// </summary>
    public int EnqueueBatch(ReadOnlySpan<T> items)
    {
        var tail = _tail.Value;
        var free = _items.Length - (int)(tail - Volatile.Read(ref _head.Value));
        var count = Math.Min(free, items.Length);
        for (var i = 0; i < count; i++)
            _items[(tail + i) & _mask] = items[i];
        if (count > 0)
            Volatile.Write(ref _tail.Value, tail + count);
        return count;
    }

    /// <summary>Consumer: takes the oldest item; false when empty.</summary>
    public bool TryDequeue(out T item)
    {
        var head = _head.Value;
        if (head == Volatile.Read(ref _tail.Value))
        {
            item = default;
            return false;
        }

        ref var slot = ref _items[head & _mask];
        item = slot;
        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
            slot = default; // drop references so the ring never keeps clips or players alive
        Volatile.Write(ref _head.Value, head + 1);
        return true;
    }

    /// <summary>Consumer: discards everything queued.</summary>
    public void Clear()
    {
        while (TryDequeue(out _))
        {
        }
    }
}

/// <summary>A 64-bit index alone on its cache line (padding on both sides) so producer and consumer do not false-share.</summary>
[StructLayout(LayoutKind.Explicit, Size = 128)]
internal struct PaddedIndex
{
    [FieldOffset(64)]
    public long Value;
}
