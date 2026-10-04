namespace MainframeEngine.Networking;

/// <summary>
/// Process-wide pool of <see cref="NetBufferWriter"/> and <see cref="NetBufferReader"/> instances. Thread-safe.
/// </summary>
/// <remarks>
/// Renting and returning never allocates in steady state: buffers keep their storage while pooled, up to
/// <see cref="MaxPooledPerKind"/> of each kind; extra returns give their storage back to the shared array pool.
/// Returning a buffer twice is ignored, so <c>Dispose</c> is idempotent.
/// </remarks>
internal static class NetBufferPool
{
    /// <summary>Most writers (and, separately, readers) kept for reuse.</summary>
    public const int MaxPooledPerKind = 64;

    private static readonly Lock Sync = new();
    private static readonly Stack<NetBufferWriter> Writers = new(MaxPooledPerKind);
    private static readonly Stack<NetBufferReader> Readers = new(MaxPooledPerKind);
    private static int _activeWriters;
    private static int _activeReaders;

    /// <summary>Writers rented and not yet returned.</summary>
    public static int ActiveWriterPoolCount
    {
        get
        {
            lock (Sync)
                return _activeWriters;
        }
    }

    /// <summary>Readers rented and not yet returned.</summary>
    public static int ActiveReaderPoolCount
    {
        get
        {
            lock (Sync)
                return _activeReaders;
        }
    }

    /// <summary>Writers waiting in the pool.</summary>
    public static int AvailableWritersCount
    {
        get
        {
            lock (Sync)
                return Writers.Count;
        }
    }

    /// <summary>Readers waiting in the pool.</summary>
    public static int AvailableReadersCount
    {
        get
        {
            lock (Sync)
                return Readers.Count;
        }
    }

    /// <summary>Rents an empty writer with at least <paramref name="capacity"/> bytes of storage.</summary>
    public static NetBufferWriter GetWriter(int capacity = NetBufferWriter.DefaultCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);
        NetBufferWriter? writer;
        lock (Sync)
        {
            _activeWriters++;
            if (Writers.TryPop(out writer))
                writer.InPool = false;
        }

        if (writer is null)
            writer = new NetBufferWriter(capacity);
        else
            writer.EnsureCapacity(capacity);
        writer.Rented = true;
        return writer;
    }

    /// <summary>Rents a reader filled with a copy of <paramref name="data"/>.</summary>
    public static NetBufferReader GetReader(ReadOnlySpan<byte> data)
    {
        NetBufferReader? reader;
        lock (Sync)
        {
            _activeReaders++;
            if (Readers.TryPop(out reader))
                reader.InPool = false;
        }

        reader ??= new NetBufferReader();
        reader.Rented = true;
        reader.SetData(data);
        return reader;
    }

    /// <summary>Rents a reader filled with a copy of the first <paramref name="length"/> bytes of <paramref name="data"/>.</summary>
    public static NetBufferReader GetReader(byte[] data, int length)
    {
        ArgumentNullException.ThrowIfNull(data);
        return GetReader(data.AsSpan(0, length));
    }

    /// <summary>Rents a reader filled with a copy of the first <paramref name="length"/> bytes of <paramref name="data"/>.</summary>
    public static NetBufferReader GetReader(ReadOnlySpan<byte> data, int length) => GetReader(data[..length]);

    /// <summary>Returns a writer (normally via <see cref="NetBufferWriter.Dispose"/>). Ignored if already pooled.</summary>
    public static void Return(NetBufferWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        lock (Sync)
        {
            if (writer.InPool)
                return;
            writer.InPool = true;
            if (writer.Rented && _activeWriters > 0)
                _activeWriters--;
            writer.Rented = false;
            writer.Reset();
            if (Writers.Count < MaxPooledPerKind)
            {
                Writers.Push(writer);
                return;
            }
        }

        writer.ReleaseStorage();
    }

    /// <summary>Returns a reader (normally via <see cref="NetBufferReader.Dispose"/>). Ignored if already pooled.</summary>
    public static void Return(NetBufferReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        lock (Sync)
        {
            if (reader.InPool)
                return;
            reader.InPool = true;
            if (reader.Rented && _activeReaders > 0)
                _activeReaders--;
            reader.Rented = false;
            reader.Reset();
            if (Readers.Count < MaxPooledPerKind)
            {
                Readers.Push(reader);
                return;
            }
        }

        reader.ReleaseStorage();
    }

    /// <summary>
    /// Empties the pool, giving every pooled buffer's storage back to the shared array pool, and resets the
    /// active counters. Buffers still rented stay usable; returning them later refills the pool.
    /// </summary>
    public static void Destroy()
    {
        lock (Sync)
        {
            while (Writers.TryPop(out var writer))
                writer.ReleaseStorage();
            while (Readers.TryPop(out var reader))
                reader.ReleaseStorage();
            _activeWriters = 0;
            _activeReaders = 0;
        }
    }
}
